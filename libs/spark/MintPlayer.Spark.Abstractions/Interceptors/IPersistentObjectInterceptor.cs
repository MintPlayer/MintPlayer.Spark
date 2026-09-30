using System.Security.Claims;

namespace MintPlayer.Spark.Abstractions.Interceptors;

/// <summary>
/// Cross-cutting behaviour around the persistent-object write and load paths — soft deletion, audit
/// stamping, locks — applied to every entity type it claims without each Actions class repeating it
/// (#460, item 1). Register with <c>AddPersistentObjectInterceptor&lt;T&gt;()</c>; scoped,
/// multi-registered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it runs:</b> in <c>IDatabaseAccess</c>, the chokepoint every framework write goes through,
/// around the Actions class's <c>OnSaveAsync</c> / <c>OnDeleteAsync</c> (D1: additive, not a pipeline
/// rewrite).
/// </para>
/// <para>
/// <b>Order:</b> before-hooks run <em>after</em> the Actions class's own <c>OnBefore*Async</c>,
/// ascending by <see cref="Order"/> and, for equal orders, in registration order; after-hooks run
/// after the Actions class's hook, in the <em>reverse</em> of that — so the first interceptor wraps
/// all the others. <see cref="PersistentObjectInterceptorOrder"/> documents the scale the built-ins use.
/// </para>
/// <para>
/// <b>Save:</b> <see cref="OnBeforeSaveAsync"/> runs inside the base <c>OnSaveAsync</c>, after mapping
/// and <c>OnBeforeSaveAsync</c>, before the WITH CHECK and the write — so it may stamp
/// <see cref="SaveContext.Entity"/> and the stamped state is what WITH CHECK judges. ⚠️ An
/// <c>OnSaveAsync</c> override that does not call the base skips before-save interceptors along with
/// WITH CHECK (D1 — documented, not fixed; a warning is logged once per type). After-save hooks
/// always run.
/// </para>
/// <para>
/// <b>Delete:</b> decided in <c>IDatabaseAccess</c>, not in the Actions class, so an
/// <c>OnDeleteAsync</c> override cannot defeat a replacement. The Actions class's
/// <c>OnBeforeDeleteAsync</c> runs first, then every <see cref="OnBeforeDeleteAsync"/>; if one calls
/// <see cref="DeleteContext.Replace"/>, the Actions class's <c>OnDeleteAsync</c> is <b>not</b> called,
/// the changes interceptors made to <see cref="DeleteContext.Entity"/> are saved instead, and
/// replication forwards a save rather than a hard delete.
/// </para>
/// <para>
/// <b>Refusing:</b> throw. The framework maps <c>SparkRowLevelAccessDeniedException</c> to 404 (not
/// visible), <c>SparkValidationException</c> to 400 (e.g. locked), and a rate-limit rejection to 429.
/// </para>
/// </remarks>
public interface IPersistentObjectInterceptor
{
    /// <summary>
    /// Whether this interceptor governs <paramref name="entityType"/>. Must depend on the type alone;
    /// the answer is cached process-wide per (interceptor type, entity type).
    /// </summary>
    bool AppliesTo(Type entityType);

    /// <summary>
    /// Where this interceptor runs among the others (contributions F5): before-hooks ascending,
    /// after-hooks descending, registration order breaking ties. Defaults to
    /// <see cref="PersistentObjectInterceptorOrder.Default"/>. Must be a constant of the type.
    /// </summary>
    int Order => PersistentObjectInterceptorOrder.Default;

    /// <summary>
    /// After an entity was loaded from RavenDB and before anything reads it (contributions F1): the
    /// seam that fills <em>satellite</em> properties — modelled, <c>[JsonIgnore]</c>d, stored in side
    /// documents — so the mapper, the row gates, the save merge and <see cref="SaveContext.Before"/>
    /// all see the hydrated entity. Called for every entity-backed load path: the base
    /// <c>LoadManyAsync</c> (every Get, Refresh, Update pre-read, parent and batched load), the base
    /// <c>OnSaveAsync</c> reload (before the posted values are merged), and the side-session load of
    /// <see cref="SaveContext.Before"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotent by contract:</b> the framework calls this at most once per entity
    /// <em>instance</em> per request. The Update endpoint pre-reads in the same session the save
    /// reloads from, so the save gets the tracked, already-hydrated instance back — and is not
    /// called again for it. Do not rely on the reason alone to decide whether to hydrate.
    /// </para>
    /// <para>
    /// ⚠️ An Actions class whose <c>OnSaveAsync</c> override does not call the base skips the save
    /// reload, so the merge sees an un-hydrated entity (a warning is logged once per type when an
    /// interceptor implements this hook).
    /// </para>
    /// </remarks>
    ValueTask OnAfterMaterializeAsync(MaterializeContext context) => ValueTask.CompletedTask;

    /// <summary>Before the write, after the Actions class's <c>OnBeforeSaveAsync</c>. May mutate <see cref="SaveContext.Entity"/>.</summary>
    ValueTask OnBeforeSaveAsync(SaveContext context) => ValueTask.CompletedTask;

    /// <summary>After the write and the Actions class's <c>OnAfterSaveAsync</c>.</summary>
    ValueTask OnAfterSaveAsync(SaveContext context) => ValueTask.CompletedTask;

    /// <summary>Before the delete, after the Actions class's <c>OnBeforeDeleteAsync</c>. May call <see cref="DeleteContext.Replace"/>.</summary>
    ValueTask OnBeforeDeleteAsync(DeleteContext context) => ValueTask.CompletedTask;

    /// <summary>After the delete — or after the replacement was saved.</summary>
    ValueTask OnAfterDeleteAsync(DeleteContext context) => ValueTask.CompletedTask;

    /// <summary>After an entity-backed persistent object was loaded through the row-gated read path.</summary>
    ValueTask OnAfterLoadAsync(LoadContext context) => ValueTask.CompletedTask;

    /// <summary>
    /// A creation of an <c>IHasNaturalId</c> type derived an id that an existing document already
    /// holds, and the row gate refused the caller that document. The framework answers 404 unless an
    /// interceptor throws its own exception here to say why (for example a
    /// <c>SparkValidationException</c>: "a deleted row holds this key — restore it instead").
    /// </summary>
    /// <remarks>
    /// ⚠️ Whatever is thrown here tells the caller something about a row it may not see. Explain only
    /// to a caller entitled to know (SoftDelete explains only to holders of <c>ViewDeleted</c> or
    /// <c>Restore</c> on the type); otherwise return and let the 404 stand.
    /// </remarks>
    ValueTask OnNaturalIdCollisionAsync(NaturalIdCollisionContext context) => ValueTask.CompletedTask;
}

/// <summary>A refused natural-id collision (see <see cref="IPersistentObjectInterceptor.OnNaturalIdCollisionAsync"/>).</summary>
public sealed class NaturalIdCollisionContext : PersistentObjectInterceptorContext
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

/// <summary>What every interceptor context carries.</summary>
public abstract class PersistentObjectInterceptorContext
{
    /// <summary>The CLR entity type.</summary>
    public required Type EntityType { get; init; }

    /// <summary>The caller, when there is an HTTP request.</summary>
    public ClaimsPrincipal? User { get; init; }

    /// <summary>Whether the caller is the system (module sync, background work) rather than a viewer.</summary>
    public bool IsSystemContext { get; init; }
}

/// <summary>A save through <c>IDatabaseAccess.SavePersistentObjectAsync</c>.</summary>
public sealed class SaveContext : PersistentObjectInterceptorContext
{
    /// <summary><see cref="PersistentObjectOperation.Save"/>, <see cref="PersistentObjectOperation.New"/>, or the explicit kind the caller passed (Revert, Restore, Sync).</summary>
    public required PersistentObjectOperation Operation { get; init; }

    /// <summary>The object as the client submitted it.</summary>
    public required PersistentObject PersistentObject { get; init; }

    /// <summary>The stored entity before this save, loaded from a separate session (so it is not the instance being saved). Null for a creation.</summary>
    public object? Before { get; init; }

    /// <summary>
    /// The entity being saved: the tracked instance in the before-hook (mutations are written), the
    /// saved instance in the after-hook. Null in a before-hook only when an <c>OnSaveAsync</c>
    /// override bypassed the base, in which case before-hooks do not run at all.
    /// </summary>
    public object? Entity { get; set; }

    /// <summary>Whether this save creates the entity.</summary>
    public bool IsNew => Operation == PersistentObjectOperation.New;
}

/// <summary>A delete through <c>IDatabaseAccess.DeletePersistentObjectAsync</c>.</summary>
public sealed class DeleteContext : PersistentObjectInterceptorContext
{
    /// <summary><see cref="PersistentObjectOperation.Delete"/>, <see cref="PersistentObjectOperation.Purge"/> or <see cref="PersistentObjectOperation.Sync"/>.</summary>
    public required PersistentObjectOperation Operation { get; init; }

    /// <summary>The document id.</summary>
    public required string Id { get; init; }

    /// <summary>The tracked entity about to be deleted. Mutations made by a replacing interceptor are saved.</summary>
    public required object Entity { get; init; }

    /// <summary>Whether this delete must be permanent. A replacing interceptor must not replace a purge.</summary>
    public bool IsPurge => Operation == PersistentObjectOperation.Purge;

    /// <summary>Whether an interceptor replaced the delete.</summary>
    public bool WasReplaced { get; private set; }

    /// <summary>
    /// Replaces the hard delete with a save of <see cref="Entity"/> as the interceptors left it: the
    /// Actions class's <c>OnDeleteAsync</c> is not called, and replication forwards a save.
    /// </summary>
    /// <exception cref="InvalidOperationException">The delete is a purge.</exception>
    public void Replace()
    {
        if (IsPurge)
            throw new InvalidOperationException($"A purge of '{Id}' cannot be replaced; it must delete the document.");
        WasReplaced = true;
    }
}

/// <summary>
/// The <see cref="IPersistentObjectInterceptor.Order"/> scale (contributions F5). Lower runs its
/// before-hooks first (and its after-hooks last), so a refusal by an earlier interceptor stops the
/// write before a later one has done anything. Gaps are deliberate: an app slots its own between.
/// </summary>
public static class PersistentObjectInterceptorOrder
{
    /// <summary>SoftDelete: turns a delete into a replacement before anything else sees it.</summary>
    public const int SoftDelete = -300;

    /// <summary>History: audit stamping and revert, after SoftDelete.</summary>
    public const int History = -200;

    /// <summary>Moderation: suspensions and locks, after History.</summary>
    public const int Moderation = -100;

    /// <summary>An interceptor that declares no order.</summary>
    public const int Default = 0;

    /// <summary>Contributions: last, so a suspension or lock refuses the save before a contribution is written.</summary>
    public const int Contributions = 100;
}

/// <summary>Why an entity was materialized (see <see cref="IPersistentObjectInterceptor.OnAfterMaterializeAsync"/>).</summary>
public enum MaterializeReason
{
    /// <summary>The row-gated read path (<c>LoadManyAsync</c>): a page, a parent, a batch.</summary>
    Load,

    /// <summary>The save's reload in the base <c>OnSaveAsync</c>, before the posted values are merged.</summary>
    SaveReload,

    /// <summary>The side-session load that becomes <see cref="SaveContext.Before"/>.</summary>
    Before,
}

/// <summary>An entity just loaded from RavenDB (see <see cref="IPersistentObjectInterceptor.OnAfterMaterializeAsync"/>).</summary>
public sealed class MaterializeContext : PersistentObjectInterceptorContext
{
    /// <summary>The loaded entity; fill its satellite properties here.</summary>
    public required object Entity { get; init; }

    /// <summary>
    /// The RavenDB <c>IAsyncDocumentSession</c> <see cref="Entity"/> was loaded in — the request
    /// session, or for <see cref="MaterializeReason.Before"/> the side session. Load side documents
    /// through it. Typed <see cref="object"/> because Abstractions (shared with the client library)
    /// carries no RavenDB dependency; <c>GetSession()</c> in <c>MintPlayer.Spark</c> returns it typed.
    /// </summary>
    public required object Session { get; init; }

    /// <summary>Which load this is.</summary>
    public required MaterializeReason Reason { get; init; }
}

/// <summary>A load through <c>IDatabaseAccess.GetPersistentObjectAsync</c> / <c>GetPersistentObjectsByIdAsync</c>.</summary>
public sealed class LoadContext : PersistentObjectInterceptorContext
{
    /// <summary>The object as it will be returned; interceptors may decorate it.</summary>
    public required PersistentObject PersistentObject { get; init; }
}
