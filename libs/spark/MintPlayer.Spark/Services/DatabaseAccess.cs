using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Reflection;
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
    [Inject] private readonly IPersistentObjectInterceptorPipeline interceptorPipeline;
    [Inject] private readonly IDisabledActionsEvaluator disabledActions;
    [Inject] private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor;
    [Inject] private readonly Microsoft.Extensions.Logging.ILogger<DatabaseAccess>? logger;

    /// <summary>Actions types whose OnSaveAsync override was seen to bypass before-save interceptors, so the warning logs once.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> bypassAnnounced = new();

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
        await session.StoreAsync(document);
        await session.SaveChangesAsync();

        // If this is a replicated entity, also broadcast the changes to the owner module
        var interceptor = serviceProvider.GetService<ISyncActionInterceptor>();
        if (interceptor != null && interceptor.IsReplicated(typeof(T)))
        {
            var idProperty = typeof(T).GetCachedProperty("Id");
            var documentId = idProperty is not null && idProperty.CanRead
                ? AccessorCache.GetGetter(idProperty)(document)?.ToString()
                : null;
            await interceptor.HandleSaveAsync(document, documentId);
        }

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
            await RunAfterLoadInterceptorsAsync(entityType, [loaded]);
        return loaded;
    }

    /// <summary>After-load interceptors (#460), in reverse registration order like every after-hook.</summary>
    private async Task RunAfterLoadInterceptorsAsync(Type entityType, IReadOnlyList<PersistentObject> objects)
    {
        var interceptors = interceptorPipeline.For(entityType);
        if (interceptors.Count == 0 || objects.Count == 0)
            return;

        var user = httpContextAccessor?.HttpContext?.User;
        var system = Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor);
        foreach (var obj in objects)
        {
            var context = new LoadContext { EntityType = entityType, PersistentObject = obj, User = user, IsSystemContext = system };
            for (var i = interceptors.Count - 1; i >= 0; i--)
                await interceptors[i].OnAfterLoadAsync(context);
        }
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
            await RunAfterLoadInterceptorsAsync(entityType, batch);
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
        await RunAfterLoadInterceptorsAsync(entityType, resolved);
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

        // Natural-id create-collision (security sweep H2): for an IHasNaturalId type the document
        // id is derived from the entity's own contents, so a "create" (Id == null) whose derived id
        // already exists is really an overwrite — and the New branch skips the Edit right, the row
        // Edit gate, the collection guard, and the concurrency check. Detect the collision and set
        // the id, so the request flows through the Edit path below (and EnsureSaveAuthorizedAsync
        // then checks "Edit", not "New"). A caller with only New rights can no longer rewrite an
        // existing document by replaying its natural key.
        if (string.IsNullOrEmpty(persistentObject.Id)
            && typeof(IHasNaturalId).IsAssignableFrom(entityType))
        {
            var probe = entityMapper.ToEntity(persistentObject) as IHasNaturalId;
            var derivedId = probe?.GetId();
            if (!string.IsNullOrEmpty(derivedId))
            {
                using var probeSession = documentStore.OpenAsyncSession();
                if (await probeSession.Advanced.ExistsAsync(derivedId))
                    persistentObject.Id = derivedId;
            }
        }

        await EnsureSaveAuthorizedAsync(persistentObject);

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
        if (!string.IsNullOrEmpty(persistentObject.Id))
        {
            using var checkSession = documentStore.OpenAsyncSession();
            var existing = await LoadEntityAsync(checkSession, entityType, persistentObject.Id);
            if (existing is not null)
            {
                // Id-to-type binding (security sweep C1/H1): the update targets an existing
                // document by a client-supplied id. If that document isn't actually of the
                // authorized type's collection, the caller is trying to overwrite a foreign
                // document (a Customer edit rewriting a SparkUser). Treat as not-found — the
                // update endpoint maps SparkRowLevelAccessDeniedException to 404. Covers the sync
                // path too: SyncActionHandler routes module writes through here.
                if (!collectionGuard.BelongsToAuthorizedCollection(checkSession, existing, entityType))
                    throw new SparkRowLevelAccessDeniedException($"Edit/{entityTypeDefinition.Name}");

                // Concurrency check folds into the same side session — see R2-M7 / M-7.
                if (!string.IsNullOrEmpty(persistentObject.Etag))
                {
                    var currentEtag = checkSession.Advanced.GetChangeVectorFor(existing);
                    if (!string.Equals(currentEtag, persistentObject.Etag, StringComparison.Ordinal))
                        throw new SparkConcurrencyException(persistentObject.Etag, currentEtag);
                }

                if (!await rowSecurity.IsAllowedAsync(entityType, "Edit", existing))
                    throw new SparkRowLevelAccessDeniedException($"Edit/{entityTypeDefinition.Name}");

                before = existing;
            }
        }

        // Disabled-action gate (#460, D13): after every row gate, so a row the caller may not see
        // stays a 404 and only a visible one can answer 403. Judged on the STORED entity (`before`),
        // never on the posted values — the hook must give the answer it gave when the page loaded.
        if (SubmittedAction(operation) is { } submitted)
            await disabledActions.EnsureEnabledAsync(entityType, submitted.Name, submitted.RefusedBy, persistentObject.Id, before);

        // Interceptors (#460, D1): registered here, after every gate, so an interceptor only ever sees
        // a save the caller was allowed to make. The before-hooks run inside the base OnSaveAsync
        // (after mapping and OnBeforeSaveAsync, before WITH CHECK and the write); the after-hooks run
        // here, in reverse order, once the Actions class returned.
        var interceptors = interceptorPipeline.For(entityType);
        SaveContext? saveContext = null;
        if (interceptors.Count > 0)
        {
            saveContext = new SaveContext
            {
                EntityType = entityType,
                Operation = operation,
                PersistentObject = persistentObject,
                Before = before,
                User = httpContextAccessor?.HttpContext?.User,
                IsSystemContext = Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor),
            };
            interceptorPipeline.BeginSave(saveContext, interceptors);
        }

        // Pass PO directly to actions — entity mapping happens inside the actions pipeline
        object savedEntity;
        var beforeHooksRan = false;
        try
        {
            savedEntity = await SaveEntityViaActionsAsync(session, entityType, persistentObject);
        }
        finally
        {
            if (saveContext is not null)
                beforeHooksRan = interceptorPipeline.EndSave(saveContext);
        }

        if (saveContext is not null)
        {
            if (!beforeHooksRan)
                AnnounceBeforeSaveBypass(entityType);

            saveContext.Entity = savedEntity;
            for (var i = interceptors.Count - 1; i >= 0; i--)
                await interceptors[i].OnAfterSaveAsync(saveContext);
        }

        // Get the generated ID from the entity
        var idProperty = entityType.GetCachedProperty("Id");
        var generatedId = idProperty is not null && idProperty.CanRead
            ? AccessorCache.GetGetter(idProperty)(savedEntity)?.ToString()
            : null;

        persistentObject.Id = generatedId;
        // Return the fresh change vector so the client can round-trip it to the next update.
        persistentObject.Etag = session.Advanced.GetChangeVectorFor(savedEntity);

        // If this is a replicated entity, also broadcast the changes to the owner module
        var interceptor = serviceProvider.GetService<ISyncActionInterceptor>();
        if (interceptor != null && interceptor.IsReplicated(entityType))
        {
            await interceptor.HandleSaveAsync(entityType, persistentObject);
        }

        return persistentObject;
    }

    public Task DeletePersistentObjectAsync(Guid objectTypeId, string id)
        => DeletePersistentObjectAsync(objectTypeId, id, PersistentObjectOperation.Delete);

    public async Task DeletePersistentObjectAsync(Guid objectTypeId, string id, PersistentObjectOperation operation)
    {
        if (operation is not (PersistentObjectOperation.Delete or PersistentObjectOperation.Purge or PersistentObjectOperation.Sync))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "A delete is a Delete, a Purge or a Sync.");

        var entityTypeDefinition = modelLoader.GetEntityType(objectTypeId);
        if (entityTypeDefinition == null) return;

        await permissionService.EnsureAuthorizedAsync("Delete", entityTypeDefinition.Name);

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

            if (!await rowSecurity.IsAllowedAsync(entityType, "Delete", existing))
                throw new SparkRowLevelAccessDeniedException($"Delete/{entityTypeDefinition.Name}");

            // Disabled-action gate (#460, D13), after the row gate, on the stored entity.
            if (SubmittedAction(operation) is { } submitted)
                await disabledActions.EnsureEnabledAsync(entityType, submitted.Name, submitted.RefusedBy, id, existing);
        }

        var interceptors = interceptorPipeline.For(entityType);
        var syncInterceptor = serviceProvider.GetService<ISyncActionInterceptor>();
        var replicated = syncInterceptor != null && syncInterceptor.IsReplicated(entityType);

        if (interceptors.Count == 0)
        {
            // Delete locally first (includes before hook)
            await DeleteEntityViaActionsAsync(session, entityType, id);

            // If this is a replicated entity, also notify the owner module
            if (replicated)
                await syncInterceptor!.HandleDeleteAsync(entityType, id);
            return;
        }

        // Interceptors (#460, D1): the replacement is decided HERE, not in the Actions class, so an
        // OnDeleteAsync override cannot defeat it (spike S4). Order: the Actions class's
        // OnBeforeDeleteAsync, then every interceptor's before-hook in registration order; then either
        // the replacement is saved or the Actions class deletes; then the after-hooks in reverse.
        var actions = actionsResolver.ResolveForType(entityType);
        var entity = await LoadEntityAsync(session, entityType, id);
        if (entity is null) return;

        await InvokeBeforeDeleteHookAsync(actions, entityType, entity);
        interceptorPipeline.MarkBeforeDeleteHandled(entity);

        var context = new DeleteContext
        {
            EntityType = entityType,
            Operation = operation,
            Id = id,
            Entity = entity,
            User = httpContextAccessor?.HttpContext?.User,
            IsSystemContext = Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor),
        };

        try
        {
            foreach (var interceptor in interceptors)
                await interceptor.OnBeforeDeleteAsync(context);

            if (context.WasReplaced)
            {
                // The entity is tracked by the request session, so whatever the interceptors set on it
                // is what gets written. Replication must forward that save — a hard delete sent for a
                // soft one would destroy the owner module's copy.
                await session.SaveChangesAsync();
                if (replicated)
                    await syncInterceptor!.HandleSaveAsync(entity, id);
            }
            else
            {
                await DeleteEntityViaActionsAsync(session, entityType, id);
                if (replicated)
                    await syncInterceptor!.HandleDeleteAsync(entityType, id);
            }
        }
        finally
        {
            // Not consumed when the Actions class's override never reached the base OnDeleteAsync.
            interceptorPipeline.ConsumeBeforeDeleteHandled(entity);
        }

        for (var i = interceptors.Count - 1; i >= 0; i--)
            await interceptors[i].OnAfterDeleteAsync(context);
    }

    /// <summary>The Actions class's <c>OnBeforeDeleteAsync(T)</c>, when it has one.</summary>
    private static async Task InvokeBeforeDeleteHookAsync(object actions, Type entityType, object entity)
    {
        var method = ReflectionCache.GetOrAdd<(string Op, Type Actions, Type Entity), MethodInfo?>(
            ("DatabaseAccess.OnBeforeDeleteAsync", actions.GetType(), entityType),
            static k => k.Actions.GetMethod("OnBeforeDeleteAsync", [k.Entity]));
        if (method is null)
            return;

        var task = (Task)method.Invoke(actions, HookInvoke, binder: null, parameters: [entity], culture: null)!;
        await task;
    }

    private void AnnounceBeforeSaveBypass(Type entityType)
    {
        if (!bypassAnnounced.TryAdd(entityType, true))
            return;

        logger?.LogWarning(
            "Before-save interceptors did not run for {EntityType}: its Actions class overrides OnSaveAsync " +
            "without calling the base implementation. That override takes over before-save interceptors " +
            "along with WITH CHECK (#460, D1). After-save interceptors still run.",
            entityType.Name);
    }

    /// <summary>
    /// The action a write submits, for the disabled-action gate (#460, D13), and every name that
    /// refuses it. <c>Save</c> is Vidyano's name for committing either an edit or a create, so a
    /// hook that disables it refuses both. Null for <see cref="PersistentObjectOperation.Sync"/>: a
    /// module sync is the system writing, not a user submitting an action.
    /// </summary>
    private static (string Name, string[] RefusedBy)? SubmittedAction(PersistentObjectOperation operation) => operation switch
    {
        PersistentObjectOperation.Save => ("Edit", ["Edit", "Save"]),
        PersistentObjectOperation.New => ("New", ["New", "Save"]),
        PersistentObjectOperation.Delete => ("Delete", ["Delete"]),
        PersistentObjectOperation.Sync => null,
        _ => (operation.ToString(), [operation.ToString()]),
    };

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

    /// <summary>
    /// Dispatches to the Actions class's virtual <c>IsAllowedAsync(string, T)</c> via reflection,
    /// so H-2/H-3 row-level authorization fires regardless of entity type.
    /// </summary>

    private async Task<object> SaveEntityViaActionsAsync(IAsyncDocumentSession session, Type entityType, PersistentObject obj)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onSaveMethod = GetCachedActionMethod(actions.GetType(), "OnSaveAsync");
        var task = (Task)onSaveMethod.Invoke(actions, HookInvoke, binder: null, parameters: [session, obj], culture: null)!;
        await task;
        return task.GetCompletedTaskResult()!;
    }

    private async Task DeleteEntityViaActionsAsync(IAsyncDocumentSession session, Type entityType, string id)
    {
        var actions = actionsResolver.ResolveForType(entityType);
        var onDeleteMethod = GetCachedActionMethod(actions.GetType(), "OnDeleteAsync");
        var task = (Task)onDeleteMethod.Invoke(actions, HookInvoke, binder: null, parameters: [session, id], culture: null)!;
        await task;
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
