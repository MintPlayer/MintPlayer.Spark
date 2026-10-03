using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Exceptions;
using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using System.Reflection;

using static MintPlayer.Spark.Services.SparkHookInvocation;

namespace MintPlayer.Spark.Services;

[Register(typeof(IDatabaseAccess), ServiceLifetime.Scoped)]
internal partial class DatabaseAccess : IDatabaseAccess
{
    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IActionsResolver actionsResolver;
    [Inject] private readonly IServiceProvider serviceProvider;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IReferenceResolver referenceResolver;
    [Inject] private readonly Breadcrumb.IBreadcrumbResolver breadcrumbResolver;
    [Inject] private readonly IRowSecurity rowSecurity;
    [Inject] private readonly ICollectionGuard collectionGuard;
    [Inject] private readonly ISparkTypeResolver typeResolver;
    [Inject] private readonly ISparkHookPipeline hooks;
    [Inject] private readonly IDisabledActionsEvaluator disabledActions;
    [Inject] private readonly IAttributeWriteShield attributeWriteShield;
    [Inject] private readonly ISaveValidation saveValidation;
    [Inject] private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor;
    [Inject] private readonly Microsoft.Extensions.Logging.ILogger<DatabaseAccess>? logger;

    public async Task<T?> GetDocumentUncheckedAsync<T>(string id) where T : class
    {
        return await session.LoadAsync<T>(id);
    }

    public async Task<IEnumerable<T>> GetDocumentsUncheckedAsync<T>() where T : class
    {
        return await session.Query<T>().ToListAsync();
    }

    public async Task<IEnumerable<T>> GetDocumentsByObjectTypeIdUncheckedAsync<T>(Guid objectTypeId) where T : class
    {
        return await session.Query<T>()
            .Where(x => ((PersistentObject)(object)x).ObjectTypeId == objectTypeId)
            .ToListAsync();
    }

    public async Task<T> SaveDocumentUncheckedAsync<T>(T document) where T : class
    {
        var interceptor = serviceProvider.GetService<ISyncActionInterceptor>();
        var replicated = interceptor != null && interceptor.IsReplicated(typeof(T));
        var idProperty = typeof(T).GetCachedProperty("Id");
        string? ReadId() => idProperty is not null && idProperty.CanRead
            ? AccessorCache.GetGetter(idProperty)(document)?.ToString()
            : null;

        // Insert or Update for the owner module (#467, D15), decided before the store assigns an id.
        var idBefore = ReadId();
        var isNew = replicated && (string.IsNullOrEmpty(idBefore) || !await session.Advanced.ExistsAsync(idBefore));

        await session.StoreAsync(document);
        await session.SaveChangesAsync();

        // If this is a replicated entity, also broadcast the changes to the owner module
        if (replicated)
            await interceptor!.HandleSaveAsync(document, ReadId(), isNew);

        return document;
    }

    public async Task DeleteDocumentUncheckedAsync<T>(string id) where T : class
    {
        session.Delete(id);
        await session.SaveChangesAsync();

        // If this is a replicated entity, also notify the owner module
        var interceptor = serviceProvider.GetService<ISyncActionInterceptor>();
        if (interceptor != null && interceptor.IsReplicated(typeof(T)))
        {
            await interceptor.HandleDeleteAsync(typeof(T), id);
        }
    }

    // PersistentObject-specific methods that handle entity mapping

    public async Task<PersistentObject?> GetPersistentObjectAsync(Guid objectTypeId, string id)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(objectTypeId);
        if (entityTypeDefinition == null) return null;

        await permissionService.EnsureAuthorizedAsync("Read", entityTypeDefinition.Name);

        // The load contract (#324): id in, page out — the type's Actions class owns everything
        // through OnLoadAsync(id, parent). For an entity-backed type the default base runs the
        // entity pipeline (document load, collection guard, row security, breadcrumbs, mapping,
        // redaction, etag); a JSON-only virtual type's name-resolved actions scaffold via
        // IManager and fill the values directly. What comes back is what the page renders; null
        // is 404.
        var entityType = typeResolver.Resolve(entityTypeDefinition.ClrType);
        if (entityType == null)
            return await LoadVirtualObjectViaActionsAsync(entityTypeDefinition, id);

        var actions = actionsResolver.ResolveForType(entityType);
        var onLoadMethod = GetCachedActionMethod(actions.GetType(), "OnLoadAsync");
        // HookInvoke is load-bearing, not tidiness — see SparkHookInvocation. This site is where the
        // omission was finally caught, by the OnLoadAsync retry row.
        var task = (Task)onLoadMethod.Invoke(actions, HookInvoke, binder: null, parameters: [id, null], culture: null)!;
        await task;
        var loaded = (PersistentObject?)task.GetCompletedTaskResult();
        if (loaded is not null)
            await hooks.RunAfterLoadAsync(entityType, session, [loaded]);
        return loaded;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PersistentObject>> GetPersistentObjectsByIdAsync(Guid objectTypeId, IReadOnlyList<string> ids)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(objectTypeId);
        if (entityTypeDefinition == null) return [];
        if (ids.Count == 0) return [];

        // One type-level decision for the whole set — PermissionService memoizes per request, so
        // this costs the same as the singular path did for one id.
        await permissionService.EnsureAuthorizedAsync("Read", entityTypeDefinition.Name);

        var entityType = typeResolver.Resolve(entityTypeDefinition.ClrType);
        if (entityType == null)
        {
            // A JSON-only virtual type has no documents to batch: its rows are composed, one call to
            // the name-resolved hook per id. Batching would be a lie about where the cost is.
            var composed = new List<PersistentObject>(ids.Count);
            foreach (var id in ids)
            {
                var obj = await LoadVirtualObjectViaActionsAsync(entityTypeDefinition, id);
                if (obj is not null) composed.Add(obj);
            }
            return composed;
        }

        var actions = actionsResolver.ResolveForType(entityType);

        // Batching is an optimization over the BASE pipeline, so it applies only where it is
        // invisible. An actions class that overrides OnLoadAsync decorates the page, and taking the
        // batched path would skip that decoration — making a row's content depend on how many rows
        // were asked for. SupportsBatchedLoad is false for exactly those, and they fall through to
        // the per-id loop below: slower, and correct.
        if (actions is Actions.IBatchedLoadActions { SupportsBatchedLoad: true } batched)
        {
            var batch = await batched.LoadManyAsync(ids, null);
            await hooks.RunAfterLoadAsync(entityType, session, batch);
            return batch;
        }

        var onLoadMethod = GetCachedActionMethod(actions.GetType(), "OnLoadAsync");
        var resolved = new List<PersistentObject>(ids.Count);
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var task = (Task)onLoadMethod.Invoke(actions, HookInvoke, binder: null, parameters: [id, null], culture: null)!;
            await task;
            if ((PersistentObject?)task.GetCompletedTaskResult() is { } obj)
                resolved.Add(obj);
        }
        await hooks.RunAfterLoadAsync(entityType, session, resolved);
        return resolved;
    }

    /// <summary>
    /// Applies the Actions class's row-level read gate to a materialized list. When the
    /// query ran against a projection type, we load the corresponding base entities from
    /// the session (Raven reuses its cache, so this is cheap for documents already seen)
    /// and evaluate the filter against those — the Actions class is typed on the base
    /// entity, not the projection.
    /// </summary>
    /// <summary>
    /// Answers "may this caller save this object?" without saving it.
    /// <para>
    /// Exists so an endpoint can ask <b>before</b> spending work on the request — specifically
    /// before validating it. Validation used to run first (N23), so a caller with no right to
    /// create an entity type received a 400 listing that type's validation errors and only reached
    /// 401/403 when the payload happened to be well-formed. The refusal was never in doubt; what
    /// leaked was which attributes a type requires, to someone who cannot create one.
    /// </para>
    /// <para>
    /// This is not a second copy of the rule. <see cref="SavePersistentObjectAsync"/> calls this
    /// same method, so there is one implementation of the decision and the chokepoint remains
    /// authoritative — the endpoint merely asks it earlier.
    /// </para>
    /// </summary>
    public async Task EnsureSaveAuthorizedAsync(PersistentObject persistentObject)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(persistentObject.ObjectTypeId)
            ?? throw new InvalidOperationException($"Could not find EntityType with ID '{persistentObject.ObjectTypeId}'");

        // Id decides the verb: absent means this is a creation, present means an edit.
        var action = string.IsNullOrEmpty(persistentObject.Id) ? "New" : "Edit";
        await permissionService.EnsureAuthorizedAsync(action, entityTypeDefinition.Name);
    }

    public Task<PersistentObject> SavePersistentObjectAsync(PersistentObject persistentObject)
        => SavePersistentObjectAsync(persistentObject, PersistentObjectOperation.Save);

    public async Task<PersistentObject> SavePersistentObjectAsync(PersistentObject persistentObject, PersistentObjectOperation operation)
    {
        if (operation is PersistentObjectOperation.Delete or PersistentObjectOperation.Purge)
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "A save cannot be a delete or a purge.");

        var entityTypeDefinition = modelLoader.GetEntityType(persistentObject.ObjectTypeId)
            ?? throw new InvalidOperationException($"Could not find EntityType with ID '{persistentObject.ObjectTypeId}'");

        var entityType = typeResolver.Resolve(entityTypeDefinition.ClrType)
            ?? throw new InvalidOperationException($"Could not resolve type '{entityTypeDefinition.ClrType}'");

        // A restore names an existing document; it never creates one (#460, SoftDelete).
        var isRestore = operation == PersistentObjectOperation.Restore;
        if (isRestore && string.IsNullOrEmpty(persistentObject.Id))
            throw new ArgumentException("A restore must name the document it restores.", nameof(persistentObject));

        // A revert, likewise, rewrites an existing document from one of its revisions (#460, History).
        var isRevert = operation == PersistentObjectOperation.Revert;
        if (isRevert && string.IsNullOrEmpty(persistentObject.Id))
            throw new ArgumentException("A revert must name the document it reverts.", nameof(persistentObject));

        // Natural-id create-collision (security sweep H2): for an IHasNaturalId type the document
        // id is derived from the entity's own contents, so a "create" (Id == null) whose derived id
        // already exists is really an overwrite — and the New branch skips the Edit right, the row
        // Edit gate, the collection guard, and the concurrency check. Detect the collision and set
        // the id, so the request flows through the Edit path below (and EnsureSaveAuthorizedAsync
        // then checks "Edit", not "New"). A caller with only New rights can no longer rewrite an
        // existing document by replaying its natural key. Since #467 (D16) the collision never
        // writes: past the Edit gates it is a 409 "already exists", because an edit must carry the
        // etag of the version it overwrites and a create has none.
        var naturalIdCollision = false;
        IReadOnlyList<string> refusedBeforeProbe = [];
        if (string.IsNullOrEmpty(persistentObject.Id)
            && typeof(IHasNaturalId).IsAssignableFrom(entityType))
        {
            // The probe maps what the create will write, not what was posted (contributions M2d): a
            // value for a New-denied attribute is dropped first, so it can neither choose the derived
            // id — rewriting the row that holds it — nor make the collision answer an existence
            // oracle for it. This is the create shield (no stored row, so static New rights only; the
            // per-row hook is not consulted), applied to the object itself so the probe, the gates and
            // the save all see the same values. Before the type-level New gate below, which is safe:
            // it never refuses, so it answers nothing. The shield call after the gates stays where it
            // is, before the hooks; it is idempotent, and on a collision it adds the Edit rules
            // judged on the stored row.
            refusedBeforeProbe = (await attributeWriteShield.ApplyAsync(persistentObject, entityTypeDefinition, entityType, stored: null)).Refused;

            var probe = entityMapper.ToEntity(persistentObject) as IHasNaturalId;
            var derivedId = probe?.GetId();
            if (!string.IsNullOrEmpty(derivedId))
            {
                using var probeSession = documentStore.OpenAsyncSession();
                if (await probeSession.Advanced.ExistsAsync(derivedId))
                {
                    persistentObject.Id = derivedId;
                    naturalIdCollision = true;
                }
            }
        }

        // A restore is its own right (Restore/T), not Edit: restoring is a moderator's act, and the
        // row it targets is one an ordinary Edit may not even see (#460, SoftDelete).
        if (isRestore)
            await permissionService.EnsureAuthorizedAsync("Restore", entityTypeDefinition.Name);
        else
        {
            // A revert is an edit (Edit/T) that also needs its own right (Revert/T): it rewrites every
            // model attribute at once, from content the caller did not type (#460, History).
            if (isRevert)
                await permissionService.EnsureAuthorizedAsync("Revert", entityTypeDefinition.Name);
            await EnsureSaveAuthorizedAsync(persistentObject);
        }

        // Save vs New follows the id (after the natural-id collision above may have set it); an
        // explicit kind — Revert, Restore, Sync — is what the caller said.
        if (operation is PersistentObjectOperation.Save or PersistentObjectOperation.New)
            operation = string.IsNullOrEmpty(persistentObject.Id) ? PersistentObjectOperation.New : PersistentObjectOperation.Save;
        object? before = null;

        // Row-level Edit gate (R2-H2): for an update against an existing entity, the
        // Actions class's IsAllowedAsync(Edit, entity) hook decides whether THIS caller
        // can edit THIS instance. Round 1's H-2 fix only covered the Read/Query paths;
        // writes silently inherited "if you can read it, you can overwrite it" — Alice
        // could overwrite Bob's records if she could read them. We load the existing
        // entity through a side session (same session as the etag check) so the row
        // gate sees the pre-update state. New entities (Id == null) skip the gate —
        // there's no instance yet to filter on; the entity-type-level "New" check
        // above is sufficient.
        //
        // A restore is gated under its own action name, so a row policy can say "only a deleted row
        // can be restored" while Edit keeps hiding deleted rows.
        if (!string.IsNullOrEmpty(persistentObject.Id))
        {
            var rowAction = isRestore ? "Restore" : isRevert ? "Revert" : "Edit";
            using var checkSession = documentStore.OpenAsyncSession();
            var existing = await LoadEntityAsync(checkSession, entityType, persistentObject.Id);
            // Hydrated like every other load (contributions F1), in the side session it came from,
            // so the row gates and SaveContext.Before see the satellite rows as they are stored.
            if (existing is not null)
                await hooks.RunAfterMaterializeAsync(entityType, checkSession, [existing], MaterializeReason.Before);
            if (existing is null && (isRestore || isRevert))
                throw new SparkRowLevelAccessDeniedException($"{rowAction}/{entityTypeDefinition.Name}");
            // An edit of a version the caller loaded (it carries an etag), whose document is gone:
            // deleted since it was loaded (#467, D15) — a 409, never a resurrection. Without an etag a
            // missing id is a create under a caller-chosen id, which only an internal caller can make:
            // every HTTP update requires an etag (D16), and a create posts no id.
            if (existing is null && !string.IsNullOrEmpty(persistentObject.Etag) && !naturalIdCollision)
                throw SparkConcurrencyException.DeletedSinceLoaded(persistentObject.Etag);
            if (existing is not null)
            {
                // Id-to-type binding (security sweep C1/H1): the update targets an existing
                // document by a client-supplied id. If that document isn't actually of the
                // authorized type's collection, the caller is trying to overwrite a foreign
                // document (a Customer edit rewriting a SparkUser). Treat as not-found — the
                // update endpoint maps SparkRowLevelAccessDeniedException to 404. Covers the sync
                // path too: SyncActionHandler routes module writes through here.
                if (!collectionGuard.BelongsToAuthorizedCollection(checkSession, existing, entityType))
                    throw new SparkRowLevelAccessDeniedException($"{rowAction}/{entityTypeDefinition.Name}");

                // Concurrency check folds into the same side session — see R2-M7 / M-7. It is the
                // fast refusal, not the guarantee: a stale etag answers 409 before any hook runs.
                // The guarantee is the framework write carrying the expected change vector
                // (contributions F7, WriteSaveAsync), which also closes the window between this check
                // and the write.
                if (!string.IsNullOrEmpty(persistentObject.Etag))
                {
                    var currentEtag = checkSession.Advanced.GetChangeVectorFor(existing);
                    if (!string.Equals(currentEtag, persistentObject.Etag, StringComparison.Ordinal))
                        throw new SparkConcurrencyException(persistentObject.Etag, currentEtag);
                }

                if (!await rowSecurity.IsAllowedAsync(entityType, rowAction, existing))
                {
                    // A creation whose natural id is held by a row this caller may not edit (a
                    // soft-deleted one, say). Still a 404 by default; an interceptor that owns the
                    // reason may explain it instead (SoftDelete: "restore it rather than create it").
                    if (naturalIdCollision)
                        await ExplainNaturalIdCollisionAsync(entityType, persistentObject, existing);
                    throw new SparkRowLevelAccessDeniedException($"{rowAction}/{entityTypeDefinition.Name}");
                }

                // A create whose natural id is held by a row this caller may edit (#467, D16): it was an
                // edit of that row with no etag — an overwrite of a version nobody saw. A 409 instead,
                // after the row gate, so a row the caller may not edit still answers as before.
                if (naturalIdCollision)
                    throw SparkConcurrencyException.AlreadyExists();

                before = existing;
            }
        }

        // Disabled-action gate (#460, D13): after every row gate, so a row the caller may not see
        // stays a 404 and only a visible one can answer 403. Judged on the STORED entity (`before`),
        // never on the posted values — the hook must give the answer it gave when the page loaded.
        if (SubmittedAction(operation) is { } submitted)
            await disabledActions.EnsureEnabledAsync(entityType, submitted.Name, submitted.RefusedBy, persistentObject.Id, before);

        // Attribute-level write rights (contributions M2c-2b): drop every posted attribute the caller
        // may not write — static Edit (New on a create) refusals on every attribute kind, and what the
        // per-row hook protects on the stored row — before anything maps, hooks or intercepts the
        // object. After every gate, so a refused save never consults the hook; judged on the STORED
        // row (`before`), like the disabled-action gate. Never refuses: that would name the attributes.
        var shield = await attributeWriteShield.ApplyAsync(persistentObject, entityTypeDefinition, entityType, before);
        IReadOnlyList<string> unwritable = refusedBeforeProbe.Count == 0
            ? shield.Refused
            : [.. refusedBeforeProbe.Concat(shield.Refused).Distinct(StringComparer.OrdinalIgnoreCase)];

        // Validation the endpoint asked for (contributions M2d): on the shielded values, skipping what
        // the caller may not write, after every gate and before anything hooks, intercepts or writes.
        await saveValidation.ValidateAsync(persistentObject, entityTypeDefinition, before, shield.Unwritable);

        // The framework owns the write (#482): hooks only ever see a save the caller was allowed to make,
        // and nothing an Actions class or a hook does can skip WITH CHECK, the expected change vector or
        // the single commit. Everything the request session tracks before the hooks run is recorded, so
        // a refusal (or a cancel) can take back what they wrote besides the target (contributions F6).
        var sessionBefore = SessionWriteSnapshot.Take(session);
        SaveContext saveContext;
        try
        {
            saveContext = await WriteSaveAsync(entityType, persistentObject, operation, before, unwritable);
            await session.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // The save loaded the row into the REQUEST session and mapped the posted values onto it before
            // a hook or WITH CHECK refused (#460, M7 finding). Left tracked, the request's next
            // SaveChangesAsync — another save, a custom action's own write — would commit the refused
            // change. Evicted, the next load in this request reads what is stored.
            await EvictTrackedAsync(entityType, persistentObject.Id);
            sessionBefore.EvictWrittenSince();

            // The write found the document changed since it was loaded (contributions F7): the same
            // conflict the etag check above answers, caught one step later, so the same 409. RavenDB's
            // message carries change vectors; it stays in the inner exception, for logs only.
            if (ex is Raven.Client.Exceptions.ConcurrencyException)
                throw new SparkConcurrencyException(ex);
            throw;
        }

        var savedEntity = saveContext.Entity;
        var idProperty = entityType.GetCachedProperty("Id");
        var generatedId = idProperty is not null && idProperty.CanRead
            ? AccessorCache.GetGetter(idProperty)(savedEntity)?.ToString()
            : null;

        persistentObject.Id = generatedId;
        saveContext.Id = generatedId;
        // Return the fresh change vector so the client can round-trip it to the next update.
        persistentObject.Etag = session.Advanced.GetChangeVectorFor(savedEntity);

        await hooks.RunIsolatedAsync(hooks.For<IAfterSave>(entityType, operation), hook => hook.OnAfterSaveAsync(saveContext), entityType, generatedId);
        return persistentObject;
    }

    public Task DeletePersistentObjectAsync(Guid objectTypeId, string id)
        => DeletePersistentObjectAsync(objectTypeId, id, PersistentObjectOperation.Delete);

    public async Task DeletePersistentObjectAsync(Guid objectTypeId, string id, PersistentObjectOperation operation, string? etag = null)
    {
        if (operation is not (PersistentObjectOperation.Delete or PersistentObjectOperation.Purge or PersistentObjectOperation.Sync))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "A delete is a Delete, a Purge or a Sync.");

        var entityTypeDefinition = modelLoader.GetEntityType(objectTypeId);
        if (entityTypeDefinition == null) return;

        // A purge is its own right (Purge/T) and its own row-gate action, so a row policy can confine
        // it to rows that are already soft-deleted while Delete keeps hiding them (#460, SoftDelete).
        var deleteAction = operation == PersistentObjectOperation.Purge ? "Purge" : "Delete";
        await permissionService.EnsureAuthorizedAsync(deleteAction, entityTypeDefinition.Name);

        var clrType = entityTypeDefinition.ClrType;
        var entityType = typeResolver.Resolve(clrType);
        if (entityType == null) return;

        // Row-level Delete gate (R2-H2): the same shape as the Edit gate in
        // SavePersistentObjectAsync — load the entity in a side session and ask the Actions class.
        // Apps can permit Read-everyone but Delete-owner-only.
        //
        // It said "side session" and used the request session, which made the two gates different
        // while claiming they were the same. The request session may already be tracking this
        // document — the delete endpoint reads it through the gated read path first — so judging its
        // copy judges whatever that copy has become, while a gate should judge what is STORED. The
        // difference is invisible today because nothing mutates between the read and the delete, and
        // it is exactly the kind of "invisible today" that stops being true after an unrelated edit.
        using (var checkSession = documentStore.OpenAsyncSession())
        {
            var existing = await LoadEntityAsync(checkSession, entityType, id);
            if (existing is null) return; // Nothing to delete; preserves 404-on-missing semantics.

            // Id-to-type binding (security sweep C1/H1): don't let a Delete on one type erase a
            // document of another by naming its id. A foreign-collection document is "not found".
            if (!collectionGuard.BelongsToAuthorizedCollection(checkSession, existing, entityType))
                return;

            if (!await rowSecurity.IsAllowedAsync(entityType, deleteAction, existing))
                throw new SparkRowLevelAccessDeniedException($"{deleteAction}/{entityTypeDefinition.Name}");

            // Disabled-action gate (#460, D13), after the row gate, on the stored entity.
            if (SubmittedAction(operation) is { } submitted)
                await disabledActions.EnsureEnabledAsync(entityType, submitted.Name, submitted.RefusedBy, id, existing);

            // The version the caller saw (#467, D14), after every gate: a row changed since is a 409
            // before any hook runs. The write below carries it too, which closes the window after this.
            if (!string.IsNullOrEmpty(etag))
            {
                var current = checkSession.Advanced.GetChangeVectorFor(existing);
                if (!string.Equals(current, etag, StringComparison.Ordinal))
                    throw new SparkConcurrencyException(etag, current);
            }
        }

        // Everything the request session tracks before any delete hook runs, so a refusal can take back
        // what the hooks wrote besides the target (contributions F6).
        var sessionBefore = SessionWriteSnapshot.Take(session);
        var entity = await LoadEntityAsync(session, entityType, id);
        if (entity is null) return;
        // The write must still find the version the caller saw (#467, D14) — or, for an internal caller,
        // the version loaded here — or a concurrent edit that landed after the gate is lost (F7).
        var expectedChangeVector = string.IsNullOrEmpty(etag) ? session.Advanced.GetChangeVectorFor(entity) : etag;

        DeleteContext context;
        try
        {
            context = await WriteDeleteAsync(entityType, id, entity, operation, reason: null, expectedChangeVector!);
            await session.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // A refusal after an earlier hook already changed the entity (SoftDelete marks it deleted,
            // then a later hook says no) must leave nothing behind for a later SaveChangesAsync in this
            // request (#460, M6 finding) — nor any other document a hook stored on the way (F6).
            session.Advanced.Evict(entity);
            sessionBefore.EvictWrittenSince();
            if (ex is Raven.Client.Exceptions.ConcurrencyException)
                throw new SparkConcurrencyException(ex);
            throw;
        }

        await hooks.RunIsolatedAsync(hooks.For<IAfterDelete>(entityType, operation), hook => hook.OnAfterDeleteAsync(context), entityType, id);
    }

    /// <summary>
    /// The one refusal message for a bulk delete (#467, D18): the breadcrumb of every row that failed,
    /// with its reason when it has one, so the user knows which rows to untick. Every row named here
    /// passed the Read gate (D11), so naming it discloses nothing; breadcrumbs go through the
    /// redacting resolver, in one batched call however many rows failed.
    /// </summary>
    private async Task<string> BulkRefusalMessageAsync(
        IAsyncDocumentSession resolveSession,
        EntityTypeDefinition entityTypeDefinition,
        IReadOnlyDictionary<string, object> entities,
        IReadOnlyList<(string Id, string? Reason)> failures,
        string lead)
    {
        var roots = failures.Select(f => entities[f.Id]).ToList();
        var breadcrumbs = await breadcrumbResolver.ResolveAsync(resolveSession, roots, entityTypeDefinition, action: "Read");
        var named = failures.Select(f =>
        {
            var name = breadcrumbs.Get(f.Id) is { Length: > 0 } breadcrumb ? breadcrumb : f.Id;
            return f.Reason is { Length: > 0 } reason ? $"{name} ({reason.TrimEnd('.')})" : name;
        });
        return $"{lead} {string.Join(", ", named)}.";
    }

    /// <inheritdoc />
    public async Task DeletePersistentObjectsAsync(Guid objectTypeId, IReadOnlyList<string> ids, SparkBulkDeleteContext? context = null)
    {
        var entityTypeDefinition = modelLoader.GetEntityType(objectTypeId)
            ?? throw new SparkRowLevelAccessDeniedException($"Delete/{objectTypeId}");

        const string deleteAction = "Delete";
        await permissionService.EnsureAuthorizedAsync(deleteAction, entityTypeDefinition.Name);

        // A composed type has no documents to delete; refused like a missing row.
        var entityType = typeResolver.Resolve(entityTypeDefinition.ClrType)
            ?? throw new SparkRowLevelAccessDeniedException($"{deleteAction}/{entityTypeDefinition.Name}");

        // An empty id names no row and cannot be verified; it fails the whole request.
        if (ids.Count == 0 || ids.Any(string.IsNullOrEmpty))
            throw new SparkRowLevelAccessDeniedException($"{deleteAction}/{entityTypeDefinition.Name}");

        var distinct = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Every gate before the first write, judged on what is STORED (a side session, as the
        // single-row delete does). Never shrink silently: a missing, foreign or denied row refuses
        // the lot, indistinguishably (M-3) — deleting 198 of 200 and saying nothing is worse.
        using (var checkSession = documentStore.OpenAsyncSession())
        {
            var stored = await RowSecurity.LoadBaseDocumentsAsync(checkSession, entityType, distinct);
            foreach (var id in distinct)
            {
                if (!stored.TryGetValue(id, out var existing)
                    || !collectionGuard.BelongsToAuthorizedCollection(checkSession, existing, entityType))
                    throw new SparkRowLevelAccessDeniedException($"{deleteAction}/{entityTypeDefinition.Name}");
            }

            // D11 (#467): every row must also be READABLE — the Read right and the Read row rule. A row
            // the caller cannot see counts as missing (M-3), before any gate that could answer
            // differently for it (the Delete rule, OnDisableActionsAsync's 403).
            if (!await permissionService.IsAllowedAsync("Read", entityTypeDefinition.Name)
                || !await rowSecurity.AreAllowedAsync(checkSession, entityType, "Read", distinct))
                throw new SparkRowLevelAccessDeniedException($"{deleteAction}/{entityTypeDefinition.Name}");

            // D18 (#467): the Delete row rule, per row, so the refusal can name the rows that failed —
            // every one of them is readable, so naming it discloses nothing.
            // D14 (#467): the version of each row the caller saw. Every one is readable, so the 409 names
            // the rows that changed since the list loaded (D18).
            if (context?.Etags is { } etags)
            {
                var changed = distinct
                    .Where(id => !etags.TryGetValue(id, out var etag) || string.IsNullOrEmpty(etag)
                        || !string.Equals(checkSession.Advanced.GetChangeVectorFor(stored[id]), etag, StringComparison.Ordinal))
                    .ToList();
                if (changed.Count > 0)
                {
                    var first = changed[0];
                    throw new SparkConcurrencyException(
                        etags.GetValueOrDefault(first) ?? string.Empty,
                        checkSession.Advanced.GetChangeVectorFor(stored[first]),
                        await BulkRefusalMessageAsync(checkSession, entityTypeDefinition, stored,
                            changed.Select(id => (id, (string?)null)).ToList(),
                            "Changed by another user since the list was loaded:"));
                }
            }

            if (!await rowSecurity.AreAllowedAsync(checkSession, entityType, deleteAction, distinct))
            {
                var denied = new List<string>();
                foreach (var id in distinct)
                    if (!await rowSecurity.IsAllowedAsync(entityType, deleteAction, stored[id]))
                        denied.Add(id);
                throw new SparkActionDisabledException(deleteAction, await BulkRefusalMessageAsync(
                    checkSession, entityTypeDefinition, stored, denied.Select(id => (id, (string?)null)).ToList(),
                    "You may not delete"));
            }

            // Disabled-action gate (#460, D13), after the row gate: the query target (with its parent)
            // and every row, in one batched call; the union decides, so one row whose hook withholds
            // Delete refuses the whole request with a 403 — naming the rows that withheld it (D18).
            if (!Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor))
            {
                var actions = actionsResolver.ResolveForType(entityType);
                var items = new List<DisableActionsItem>(distinct.Length + 1);
                if (context?.Query is { } query
                    && string.Equals(query.EntityType, entityTypeDefinition.Name, StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new DisableActionsItem(new DisabledActionSet(), new DisableActionsContext
                    {
                        Phase = DisableActionsPhase.Submit,
                        TargetKind = DisableActionsTargetKind.Query,
                        ActionName = deleteAction,
                        Query = Queries.SparkQueryInfo.From(query),
                        Parent = context.Parent,
                        ParentType = context.ParentType,
                    }));
                }

                foreach (var id in distinct)
                {
                    items.Add(new DisableActionsItem(new DisabledActionSet(), new DisableActionsContext
                    {
                        Phase = DisableActionsPhase.Submit,
                        TargetKind = DisableActionsTargetKind.PersistentObject,
                        ActionName = deleteAction,
                        Id = id,
                        Entity = stored[id],
                    }));
                }

                var disabled = await disabledActions.EvaluateAsync(actions, items);
                if (disabled.Contains(deleteAction))
                {
                    // The query target withholding Delete is about the list, not a row: the plain refusal.
                    var withheldBy = items
                        .Where(item => item.Context.TargetKind == DisableActionsTargetKind.PersistentObject
                            && item.Target is DisabledActionSet set
                            && set.Names.Contains(deleteAction, StringComparer.OrdinalIgnoreCase))
                        .Select(item => item.Context.Id!)
                        .ToList();
                    if (withheldBy.Count == 0 || items.Any(item => item.Context.TargetKind == DisableActionsTargetKind.Query
                            && item.Target is DisabledActionSet q && q.Names.Contains(deleteAction, StringComparer.OrdinalIgnoreCase)))
                        throw new SparkActionDisabledException(deleteAction);

                    throw new SparkActionDisabledException(deleteAction, await BulkRefusalMessageAsync(
                        checkSession, entityTypeDefinition, stored, withheldBy.Select(id => (id, (string?)null)).ToList(),
                        "Delete is not available for"));
                }
            }
        }

        // Before the batch touches the request session (contributions F6), so a refusal evicts what
        // the hooks stored besides the rows themselves.
        var sessionBefore = SessionWriteSnapshot.Take(session);

        // One batched load into the request session: every later per-row load is an identity-map hit,
        // so 200 rows cost one request rather than blowing the session's request budget.
        var tracked = await RowSecurity.LoadBaseDocumentsAsync(session, entityType, distinct);
        // Every write carries the version the caller saw (#467, D14) — or, for an internal caller, the
        // version loaded here — so an edit landing after the checks above refuses the batch with a 409.
        var expectedChangeVectors = tracked.ToDictionary(
            pair => pair.Key,
            pair => context?.Etags?.GetValueOrDefault(pair.Key) is { Length: > 0 } etag ? etag : session.Advanced.GetChangeVectorFor(pair.Value),
            StringComparer.OrdinalIgnoreCase);
        var contexts = new List<DeleteContext>(distinct.Length);

        try
        {
            // D18 (#467): a row a hook refuses (a Moderation lock, a required reason) is recorded and the
            // rest are still judged, so one refusal names every row to untick. Nothing is committed: the
            // first refusal already decides the batch fails.
            var refused = new List<(string Id, string? Reason)>();
            foreach (var id in distinct)
            {
                try
                {
                    contexts.Add(await WriteDeleteAsync(entityType, id, tracked[id], PersistentObjectOperation.Delete, context?.Reason, expectedChangeVectors[id]));
                }
                catch (SparkValidationException ex)
                {
                    refused.Add((id, ex.Message));
                }
                catch (SparkRetryActionException)
                {
                    // A per-row prompt in a bulk delete is unworkable (#482): the row is refused instead.
                    refused.Add((id, "asks for a confirmation; delete it on its own"));
                }
            }

            if (refused.Count > 0)
                throw new SparkValidationException(await BulkRefusalMessageAsync(
                    session, entityTypeDefinition, tracked, refused, "These items cannot be deleted:"));

            // One commit for every row (#460, D18): all or nothing.
            await session.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Nothing half-made may reach a later save in this request: every row this batch touched —
            // marked soft-deleted, or queued for deletion — is evicted, so it reads as stored again. A
            // cancel (SparkCancelException) of one row cancels the batch the same way.
            foreach (var entity in tracked.Values)
                session.Advanced.Evict(entity);
            sessionBefore.EvictWrittenSince();
            if (ex is Raven.Client.Exceptions.ConcurrencyException)
                throw new SparkConcurrencyException(ex);
            throw;
        }

        var afterDelete = hooks.For<IAfterDelete>(entityType, PersistentObjectOperation.Delete);
        foreach (var deleteContext in contexts)
            await hooks.RunIsolatedAsync(afterDelete, hook => hook.OnAfterDeleteAsync(deleteContext), entityType, deleteContext.Id);
    }

    /// <summary>
    /// The framework's half of a save (#482), after every gate and before the commit: load (or not),
    /// hydrate, map through the Actions class's <c>MapAsync</c>, run the before-save hooks, WITH CHECK,
    /// and store with the expected change vector. The caller commits. Nothing here can be skipped by an
    /// Actions class or a hook.
    /// </summary>
    private async Task<SaveContext> WriteSaveAsync(
        Type entityType, PersistentObject obj, PersistentObjectOperation operation, object? before, IReadOnlyList<string> unwritable)
    {
        object? existing = null;
        // The change vector the write must still find (contributions F7): the client's etag when it
        // posted one, otherwise the version this session loaded. The etag check before this compares in
        // a side session and cannot see a write that lands after it; the write itself can. Null for a
        // create — including a create under a caller-chosen id.
        string? expectedChangeVector = null;
        if (!string.IsNullOrEmpty(obj.Id))
        {
            existing = await LoadEntityAsync(session, entityType, obj.Id);
            if (existing is not null)
            {
                expectedChangeVector = !string.IsNullOrEmpty(obj.Etag) ? obj.Etag : session.Advanced.GetChangeVectorFor(existing);
                // Satellite properties are filled before the posted values are merged (contributions F1),
                // so an edited hydrated row maps as an Edit, not a New plus a Delete. Idempotent: an
                // instance the Update pre-read already hooked is not hooked again.
                await hooks.RunAfterMaterializeAsync(entityType, session, [existing], MaterializeReason.SaveReload);
            }
            else if (!string.IsNullOrEmpty(obj.Etag))
            {
                // The caller edited a version that no longer exists (#467, D15): deleted since it was
                // loaded. Recreating it from the posted values would undo the delete.
                throw SparkConcurrencyException.DeletedSinceLoaded(obj.Etag);
            }
        }

        var entity = await MapViaActionsAsync(entityType, obj, existing);
        var context = new SaveContext
        {
            EntityType = entityType,
            Operation = operation,
            PersistentObject = obj,
            Before = before,
            UnwritableAttributes = unwritable,
            Entity = entity,
            Id = existing is null ? null : obj.Id,
            Session = session,
            User = hooks.User,
            IsSystemContext = hooks.IsSystemContext,
        };

        foreach (var hook in hooks.For<IBeforeSave>(entityType, operation))
            await hook.OnBeforeSaveAsync(context);

        // WITH CHECK — the write half of row-level security, SQL RLS's WITH CHECK to the read paths'
        // USING: judged on the entity's RESULTING state, after mapping and every before-hook (so
        // ownership stamping has happened). A create must produce a row its caller could see, and an edit
        // must not move a row into someone else's scope. The same rule every read path applies (#460, D1).
        var action = string.IsNullOrEmpty(obj.Id) ? "New" : "Edit";
        if (!await rowSecurity.IsAllowedAsync(entityType, action, entity))
            throw new SparkRowLevelAccessDeniedException($"{action}/{entityType.Name}");

        if (expectedChangeVector is not null)
            await session.StoreAsync(entity, expectedChangeVector, session.Advanced.GetDocumentId(entity));
        else
            await session.StoreAsync(entity);
        return context;
    }

    /// <summary>
    /// The framework's half of a delete (#482), after every gate and before the commit: ask the
    /// <see cref="IDeleteReplacement"/>, run the before-delete hooks, then either store the replacement or
    /// delete — both with the version the caller saw (#467, D14), so an edit landing after the gates is a
    /// 409. The caller commits.
    /// </summary>
    private async Task<DeleteContext> WriteDeleteAsync(
        Type entityType, string id, object entity, PersistentObjectOperation operation, string? reason, string expectedChangeVector)
    {
        var context = new DeleteContext
        {
            EntityType = entityType,
            Operation = operation,
            Id = id,
            Entity = entity,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            Session = session,
            User = hooks.User,
            IsSystemContext = hooks.IsSystemContext,
        };

        // Decided before any before-delete hook, so each sees the final answer (contributions and
        // replication depend on it: a soft delete must not destroy a row's contributions).
        if (!context.IsPurge && hooks.For<IDeleteReplacement>(entityType, operation) is [var replacement])
            context.IsReplaced = await replacement.ReplaceAsync(context);

        foreach (var hook in hooks.For<IBeforeDelete>(entityType, operation))
            await hook.OnBeforeDeleteAsync(context);

        if (context.IsReplaced)
        {
            // The entity is tracked by the request session, so what the hooks set on it is written.
            await session.StoreAsync(entity, expectedChangeVector, id);
        }
        else
        {
            // Evicted first: RavenDB refuses a delete by id of a tracked entity that a hook changed.
            session.Advanced.Evict(entity);
            SparkRawWrites.IssuedByFramework(session, id);
            session.Delete(id, expectedChangeVector);
        }
        return context;
    }

    /// <summary>
    /// Gives every applicable hook the chance to explain a refused natural-id collision with its own
    /// exception (a 400 that says why) before the default 404 is thrown.
    /// </summary>
    private async Task ExplainNaturalIdCollisionAsync(Type entityType, PersistentObject persistentObject, object existing)
    {
        var collisionHooks = hooks.For<INaturalIdCollision>(entityType);
        if (collisionHooks.Count == 0)
            return;

        var context = new NaturalIdCollisionContext
        {
            EntityType = entityType,
            Id = persistentObject.Id!,
            PersistentObject = persistentObject,
            Existing = existing,
            Session = session,
            User = hooks.User,
            IsSystemContext = hooks.IsSystemContext,
        };
        foreach (var hook in collisionHooks)
            await hook.OnNaturalIdCollisionAsync(context);
    }

    /// <summary>
    /// The action a write submits, for the disabled-action gate (#460, D13), and every name that
    /// refuses it. <c>Save</c> is Vidyano's name for committing either an edit or a create, so a
    /// hook that disables it refuses both. Null for <see cref="PersistentObjectOperation.Sync"/>: a
    /// module sync is the system writing, not a user submitting an action.
    /// <para>
    /// A restore is an edit of the stored row and a purge a delete of it, so a hook that withholds
    /// <c>Edit</c> (or <c>Save</c>) refuses a restore and one that withholds <c>Delete</c> refuses a
    /// purge, besides their own names (#460, M6 — the conservative reading: a row an author froze
    /// against editing is not silently rewritten by a restore). A revert is an edit too, so
    /// <c>Revert</c>, <c>Edit</c> or <c>Save</c> refuses it (M7).
    /// </para>
    /// </summary>
    private static (string Name, string[] RefusedBy)? SubmittedAction(PersistentObjectOperation operation) => operation switch
    {
        PersistentObjectOperation.Save => ("Edit", ["Edit", "Save"]),
        PersistentObjectOperation.New => ("New", ["New", "Save"]),
        PersistentObjectOperation.Delete => ("Delete", ["Delete"]),
        PersistentObjectOperation.Restore => ("Restore", ["Restore", "Edit", "Save"]),
        PersistentObjectOperation.Revert => ("Revert", ["Revert", "Edit", "Save"]),
        PersistentObjectOperation.Purge => ("Purge", ["Purge", "Delete"]),
        PersistentObjectOperation.Sync => null,
        _ => (operation.ToString(), [operation.ToString()]),
    };

    /// <summary>
    /// Evicts <paramref name="id"/> from the request session when it is tracked there. A tracked id
    /// is answered from the session's identity map, so the load costs no request.
    /// </summary>
    private async Task EvictTrackedAsync(Type entityType, string? id)
    {
        if (string.IsNullOrEmpty(id) || !session.Advanced.IsLoaded(id))
            return;
        if (await LoadEntityAsync(session, entityType, id) is { } tracked)
            session.Advanced.Evict(tracked);
    }

    private async Task<object?> LoadEntityAsync(IAsyncDocumentSession session, Type entityType, string id)
    {
        var genericMethod = ReflectionCache.GetOrAdd<(string Op, Type Type), MethodInfo?>(
            ("DatabaseAccess.SessionLoadAsync", entityType),
            static k =>
            {
                var method = typeof(IAsyncDocumentSession).GetMethod(
                    nameof(IAsyncDocumentSession.LoadAsync),
                    [typeof(string), typeof(CancellationToken)]);
                return method?.MakeGenericMethod(k.Type);
            });
        var task = genericMethod?.Invoke(session, [id, CancellationToken.None]) as Task;

        if (task == null) return null;

        await task;

        return task.GetCompletedTaskResult();
    }

    #region Actions Helper Methods

    /// <summary>
    /// The read path for a JSON-only virtual type: resolve <c>{Name}Actions</c> by the model
    /// type's name (there is no CLR type to resolve over) and invoke its load hook — duck-typed
    /// with the exact same signature every actions class has, no base class required:
    /// <code>public Task&lt;PersistentObject?&gt; OnLoadAsync(string id, PersistentObject? parent)</code>
    /// The class scaffolds its own object (the <c>IManager.GetPersistentObject</c> idiom dialogs
    /// already use), fills values and <see cref="PersistentObject.Breadcrumb"/> (the page title),
    /// and returns it — free to ignore the id. The result is served read-only (<c>Can</c> forced
    /// to none unless the hook set it) — anything interactive on such a page is a custom action
    /// with its own authorization.
    /// <para>
    /// No actions class, or no <c>OnLoadAsync</c> on it, means the type has no page: null → 404.
    /// A method named <c>OnLoadAsync</c> whose shape doesn't match throws loudly instead of
    /// silently 404ing — the contract is reflective, so this is where a typo surfaces.
    /// </para>
    /// </summary>
    private async Task<PersistentObject?> LoadVirtualObjectViaActionsAsync(
        EntityTypeDefinition entityTypeDefinition, string id)
    {
        var actions = actionsResolver.ResolveByEntityName(entityTypeDefinition.Name);
        if (actions is null)
            return null;

        var loadMethod = ReflectionCache.GetOrAdd<(string Op, Type Actions), MethodInfo?>(
            ("DatabaseAccess.VirtualLoadMethod", actions.GetType()),
            static k =>
            {
                var method = k.Actions.GetMethod("OnLoadAsync", [typeof(string), typeof(PersistentObject)]);
                if (method is not null)
                {
                    if (method.ReturnType != typeof(Task<PersistentObject?>))
                        throw new InvalidOperationException(
                            $"'{k.Actions.FullName}.OnLoadAsync' must return Task<PersistentObject?>. " +
                            $"Expected: 'Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)'.");
                    return method;
                }
                if (k.Actions.GetMethods().Any(m => m.Name == "OnLoadAsync"))
                    throw new InvalidOperationException(
                        $"'{k.Actions.FullName}' has an OnLoadAsync that doesn't match the load contract. " +
                        $"Expected: 'Task<PersistentObject?> OnLoadAsync(string id, PersistentObject? parent)'.");
                return null;
            });
        if (loadMethod is null)
            return null;

        var task = (Task)loadMethod.Invoke(actions, HookInvoke, binder: null, parameters: [id, null], culture: null)!;
        await task;
        var obj = (PersistentObject?)task.GetCompletedTaskResult();
        if (obj is null)
            return null;

        // The hook only fills values; the framework squares the envelope: the page answers to
        // the id it was requested as (unless the hook chose another), titles itself from the
        // model's breadcrumb template over the values the hook just filled (unless the hook set
        // one), and is read-only unless the hook said otherwise.
        obj.Id ??= id;
        obj.Breadcrumb ??= RenderVirtualBreadcrumb(entityTypeDefinition.Breadcrumb, obj);
        obj.Can ??= new PersistentObjectPermissions { Edit = false, Delete = false };
        return obj;
    }

    /// <summary>
    /// The virtual-type counterpart of breadcrumb resolution: no entity exists, so the model's
    /// template renders over the returned object's attribute values. Reference placeholders
    /// can't resolve here (nothing to follow) and render as empty.
    /// </summary>
    private static string? RenderVirtualBreadcrumb(string? template, PersistentObject obj)
    {
        if (string.IsNullOrEmpty(template))
            return null;

        var rendered = string.Concat(Breadcrumb.BreadcrumbTemplate.Parse(template).Select(token => token switch
        {
            Breadcrumb.LiteralToken literal => literal.Text,
            Breadcrumb.FieldToken field =>
                obj.Attributes.FirstOrDefault(a => a.Name == field.AttributeName)?.Value?.ToString() ?? string.Empty,
            _ => string.Empty,
        }));
        return string.IsNullOrWhiteSpace(rendered) ? null : rendered;
    }


    /// <summary>The Actions class's <c>MapAsync(obj, existing)</c> (#482), the only save seam it keeps.</summary>
    private async Task<object> MapViaActionsAsync(Type entityType, PersistentObject obj, object? existing)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var mapMethod = GetCachedActionMethod(actions.GetType(), "MapAsync");
        var task = (Task)mapMethod.Invoke(actions, HookInvoke, binder: null, parameters: [obj, existing], culture: null)!;
        await task;
        return task.GetCompletedTaskResult()
            ?? throw new InvalidOperationException($"'{actions.GetType().FullName}.MapAsync' returned null.");
    }

    /// <summary>
    /// Cached <c>actionsType.GetMethod(name)</c>. The actions-type+method-name pair is
    /// stable for the AppDomain (an Actions class doesn't grow new methods at runtime),
    /// so a single lookup per pair is sufficient. Throws if the named method is missing —
    /// the action plumbing requires it, so a missing method is a programming error, not
    /// a runtime condition we want to silently swallow.
    /// </summary>
    private static MethodInfo GetCachedActionMethod(Type actionsType, string methodName)
        => ReflectionCache.GetOrAdd<(string Op, Type Actions, string Method), MethodInfo>(
            ("DatabaseAccess.ActionsMethod", actionsType, methodName),
            static k => k.Actions.GetMethod(k.Method)
                ?? throw new InvalidOperationException(
                    $"Actions type '{k.Actions.FullName}' is missing required method '{k.Method}'."));

    #endregion
}
