using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The create and update sequences of <c>/spark/po/create</c> and <c>/spark/po/update</c>, in one place
/// both the HTTP endpoints and the MCP tools call (MCP PRD D9): authorize, validate, save, present.
/// </summary>
/// <remarks>
/// <para>
/// Request parsing and the envelope stay with the caller; everything that decides
/// whether a write happens is here. Refusals are exceptions, exactly the ones the save itself raises,
/// so a caller maps one set: <see cref="SparkAccessDeniedException"/> (and its row-level subclass),
/// <see cref="SparkConcurrencyException"/>, <see cref="SparkSaveValidationException"/>,
/// <see cref="SparkValidationException"/>, <see cref="SparkActionDisabledException"/> and
/// <see cref="SparkThrottledException"/>.
/// </para>
/// <para>
/// The caller sets nothing on <paramref name="obj"/> beyond the posted values: the id, the type and
/// the parent are overwritten here, because each is the caller's claim.
/// </para>
/// </remarks>
internal interface IPersistentObjectSavePath
{
    /// <summary>
    /// Creates <paramref name="obj"/> as a new <paramref name="entityType"/> and returns it as a load
    /// presents it; <see langword="null"/> when an interceptor cancelled the create (#482).
    /// </summary>
    /// <param name="parent">The sub-query parent the create was started from, already resolved and authorized.</param>
    Task<PersistentObject?> CreateAsync(EntityTypeDefinition entityType, PersistentObject obj, PersistentObject? parent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves <paramref name="obj"/> over the stored <paramref name="id"/> at the version its etag names,
    /// and returns the row as a load presents it. A cancelled save answers the row as stored (#482).
    /// </summary>
    /// <param name="retry">The request whose retry answers are accepted once the row is known to exist, as the endpoint always did; <see langword="null"/> for a first attempt.</param>
    Task<PersistentObject> UpdateAsync(EntityTypeDefinition entityType, string id, PersistentObject obj, IRetryableRequest? retry = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the stored <paramref name="id"/> at the version <paramref name="etag"/> names: the sequence of
    /// <c>/spark/po/delete</c>. <see langword="false"/> when the row is missing or not the caller's to see (one
    /// answer, M-3).
    /// </summary>
    Task<bool> DeleteAsync(EntityTypeDefinition entityType, string id, string etag, CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything <see cref="CreateAsync"/> decides — the rights, the gates, the validation — and no write
    /// (MCP <c>propose_changes</c>). Throws what the create would throw. <paramref name="obj"/> is left as the
    /// create would save it: values the caller may not set are dropped.
    /// </summary>
    Task CheckCreateAsync(EntityTypeDefinition entityType, PersistentObject obj, CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything <see cref="UpdateAsync"/> decides — the version, the rights, the row gates, the validation —
    /// and no write (MCP <c>propose_changes</c>). Throws what the update would throw.
    /// </summary>
    Task CheckUpdateAsync(EntityTypeDefinition entityType, string id, PersistentObject obj, CancellationToken cancellationToken = default);

    /// <summary>Everything <see cref="DeleteAsync"/> decides, and no delete. <see langword="false"/> as there.</summary>
    Task<bool> CheckDeleteAsync(EntityTypeDefinition entityType, string id, string etag, CancellationToken cancellationToken = default);
}

[Register(typeof(IPersistentObjectSavePath), ServiceLifetime.Scoped)]
internal sealed partial class PersistentObjectSavePath : IPersistentObjectSavePath
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly ISaveValidation saveValidation;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly ISaveResponsePresenter saveResponse;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly ISparkWriteUnit writeUnit;

    public async Task<PersistentObject?> CreateAsync(EntityTypeDefinition entityType, PersistentObject obj, PersistentObject? parent, CancellationToken cancellationToken = default)
    {
        // Still in addition to the check EnsureSaveAuthorizedAsync makes below, and still free: the
        // permission service memoises per request.
        await permissionService.EnsureAuthorizedAsync("New", entityType.Name);

        // Ensure the ObjectTypeId matches the resolved entity type
        obj.ObjectTypeId = entityType.Id;
        // R2-M18: a create is "Create", never "Edit". Force Id=null so SavePersistentObjectAsync's
        // string.IsNullOrEmpty(Id) branch always picks "New" — a client posting
        // {"Id":"cars/existing"} used to flip the action to Edit and overwrite a
        // foreign record under the POST verb, bypassing the entity-type-level "New"
        // permission and any developer mental-model of "POST = creation".
        obj.Id = null;
        // A Parent in the body is the caller's claim, so it is replaced either way — never the context a
        // hook decides on.
        obj.Parent = parent;

        // The cancel covers the whole sequence, as it always did in the endpoint.
        try
        {
            // Authorize before validating (N23). The other order told a caller with no right to
            // create this type which of its attributes were invalid — a refusal either way, but one
            // that answers a question the caller was not entitled to ask. The decision itself still
            // belongs to DatabaseAccess; this only asks it earlier.
            await databaseAccess.EnsureSaveAuthorizedAsync(obj);

            // Validated inside the save, right after the write shield (contributions M2d): against the
            // object as the refresh hook shapes it, not as the model declares it (a hook that makes a
            // field required changed the contract, and re-deriving it server-side is what stops a
            // client escaping the hook by never calling /refresh) — and only on the attributes the
            // caller may set, since a New-denied one keeps its CLR default whatever was posted.
            saveValidation.Request(obj, cancellationToken);

            // Values for attributes the caller may not set on a create are dropped inside the save
            // (contributions M2c-2b, leak 7): the CLR default or initializer stays, and the save is not
            // refused, because a refusal would say which attributes exist.
            var result = await databaseAccess.SavePersistentObjectAsync(obj);

            // Re-presented as a load presents it, as an update is (leak 2).
            return await saveResponse.PresentAsync(entityType, result, isNew: true, cancellationToken);
        }
        catch (SparkCancelException)
        {
            // An interceptor cancelled the create (#482): nothing was created, and nothing went wrong.
            return null;
        }
    }

    public async Task<PersistentObject> UpdateAsync(EntityTypeDefinition entityType, string id, PersistentObject obj, IRetryableRequest? retry = null, CancellationToken cancellationToken = default)
    {
        // An update says which version it edits (#467, D16): without one it would overwrite whatever
        // is stored now. Before any read, so the answer says nothing about the row.
        if (string.IsNullOrEmpty(obj.Etag))
            throw new ArgumentException("An update must carry the etag of the version it edits.", nameof(obj));

        // No UnescapeDataString: the id is a JSON string now, not a path segment, so it arrives
        // exactly as the client wrote it. Unescaping it here would corrupt any id that legitimately
        // contains a '%'.
        var existingObj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, id);

        // The caller loaded this object (it holds an etag) and it is gone — deleted, soft-deleted,
        // or no longer theirs to see: "deleted by another user" (#467, D15), never a resurrection.
        // One answer for all three, so it tells nothing a 404 would not (M-3).
        if (existingObj is null)
            throw SparkConcurrencyException.DeletedSinceLoaded(obj.Etag);

        RetryScope.Accept(retryAccessor, retry);

        obj.Id = existingObj.Id;
        obj.ObjectTypeId = entityType.Id;
        // Only a create resolves a parent (from a sub-query's New); a Parent in this body is the
        // caller's claim and must never reach a save hook as context.
        obj.Parent = null;

        // Authorize before validating — see the note in CreateAsync (N23).
        await databaseAccess.EnsureSaveAuthorizedAsync(obj);

        // Validated inside the save, right after the write shield (contributions M2d), as in CreateAsync —
        // and only on the attributes the caller may write, since the others keep their stored value
        // whatever was posted.
        saveValidation.Request(obj, cancellationToken);

        PersistentObject result;
        try
        {
            result = await databaseAccess.SavePersistentObjectAsync(obj);
        }
        catch (SparkCancelException)
        {
            // An interceptor cancelled the save (#482): nothing was written, so the answer is the object as
            // stored — a 200, never an error.
            result = await databaseAccess.GetPersistentObjectAsync(entityType.Id, id) ?? existingObj;
        }

        // Re-presented as a load presents it (contributions M2c-2b, leak 2): never the posted
        // object, which is the client's values plus whatever the save hooks wrote into it.
        return await saveResponse.PresentAsync(entityType, result, isNew: false, cancellationToken);
    }

    public async Task<bool> DeleteAsync(EntityTypeDefinition entityType, string id, string etag, CancellationToken cancellationToken = default)
    {
        // The row-gated read first, as the endpoint always did: a row the caller may not see is missing.
        if (await databaseAccess.GetPersistentObjectAsync(entityType.Id, id) is null)
            return false;

        await databaseAccess.DeletePersistentObjectAsync(entityType.Id, id, Abstractions.Interceptors.PersistentObjectOperation.Delete, etag);
        return true;
    }

    public async Task CheckCreateAsync(EntityTypeDefinition entityType, PersistentObject obj, CancellationToken cancellationToken = default)
    {
        // CreateAsync's sequence up to the write, in its order (N23: authorize before validating).
        await permissionService.EnsureAuthorizedAsync("New", entityType.Name);
        obj.ObjectTypeId = entityType.Id;
        obj.Id = null;
        obj.Parent = null;
        await databaseAccess.EnsureSaveAuthorizedAsync(obj);
        saveValidation.Request(obj, cancellationToken);
        await writeUnit.CheckSaveAsync(obj);
    }

    public async Task CheckUpdateAsync(EntityTypeDefinition entityType, string id, PersistentObject obj, CancellationToken cancellationToken = default)
    {
        // UpdateAsync's sequence up to the write, in its order.
        if (string.IsNullOrEmpty(obj.Etag))
            throw new ArgumentException("An update must carry the etag of the version it edits.", nameof(obj));

        var existingObj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, id)
            ?? throw SparkConcurrencyException.DeletedSinceLoaded(obj.Etag);

        obj.Id = existingObj.Id;
        obj.ObjectTypeId = entityType.Id;
        obj.Parent = null;
        await databaseAccess.EnsureSaveAuthorizedAsync(obj);
        saveValidation.Request(obj, cancellationToken);
        await writeUnit.CheckSaveAsync(obj);
    }

    public async Task<bool> CheckDeleteAsync(EntityTypeDefinition entityType, string id, string etag, CancellationToken cancellationToken = default)
    {
        if (await databaseAccess.GetPersistentObjectAsync(entityType.Id, id) is null)
            return false;

        return await writeUnit.CheckDeleteAsync(entityType.Id, id, etag);
    }
}
