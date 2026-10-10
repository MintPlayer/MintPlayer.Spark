using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Endpoints.PersistentObject;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The body of <c>POST /spark/po/load</c>, shared with the live socket (X4) so a pushed object and a loaded
/// object are one read path: row security, <c>GetProtectedAttributesAsync</c> redaction, attribute rights
/// and <c>OnDisableActionsAsync</c>.
/// </summary>
internal interface IPersistentObjectReadPath
{
    /// <summary>
    /// The object as the caller may see it, or <see langword="null"/> for every refusal alike — unknown type,
    /// missing id, missing document, denied row (#453: missing ≡ no access).
    /// </summary>
    Task<PersistentObject?> LoadAsync(PersistentObjectReferenceRequest request, CancellationToken cancellationToken);
}

[Register(typeof(IPersistentObjectReadPath), ServiceLifetime.Scoped)]
internal sealed partial class PersistentObjectReadPath : IPersistentObjectReadPath
{
    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly IDisabledActionsEvaluator disabledActions;
    [Inject] private readonly IRowPolicyRequestState rowPolicyRequestState;

    public async Task<PersistentObject?> LoadAsync(PersistentObjectReferenceRequest request, CancellationToken cancellationToken)
    {
        // The catch-all `{**id}` that used to carry the id is gone, and with it the empty-id case it
        // created (the bare "/{objectTypeId}" path matched too, and the ! on a null RouteValue was a
        // 500 rather than a refusal). An absent id in the body is now just an absent field.
        var entityType = SparkRequestType.Resolve(modelLoader, request);
        if (entityType is null || string.IsNullOrEmpty(request.Id))
            return null;

        // A load can prompt now, which is the whole reason it grew a body. OnLoadAsync raising a
        // retry gets the same 449 envelope as every other hook.
        RetryScope.Accept(retryAccessor, request);

        // T2 on the load side (#460, M7): a ViewDeleted holder opening a row from the recycle bin.
        // Set before anything asks row security, because row filters are memoized per request.
        // Only for this type: the row's references (a live question of a deleted answer) stay live.
        rowPolicyRequestState.Deleted = request.Deleted ?? SparkDeletedFilter.Exclude;
        rowPolicyRequestState.DeletedScopeClrType = entityType.ClrType;

        try
        {
            // No UnescapeDataString: see the note in Update.
            var obj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, request.Id);
            if (obj is null)
                return null;

            // The page-load half of D13 (#460): the actions class's OnDisableActionsAsync decides what
            // this object withholds, and the answer travels on the object's disabledActions — the
            // field the client already reads, so the wire is unchanged. The same hook is asked again
            // when an action is submitted, which is what makes the answer enforceable.
            //
            // Here, on the page load, and not inside GetPersistentObjectAsync: that method is also the
            // row-gated read every mutating path starts with, and those never render the object.
            await disabledActions.ApplyOnLoadAsync(obj);

            // Static attribute rights (contributions M2c-2a) are no longer applied here: the object was
            // built for this caller (D13a) — a Read-denied attribute absent, an Edit-denied one
            // read-only — before OnLoadAsync saw it, and the boundary net checks it on the way out.
            // This is also what the M1c conflict dialog re-fetches, so a removed attribute can never
            // reach it.
            return obj;
        }
        catch (SparkAccessDeniedException)
        {
            return null;
        }
    }
}
