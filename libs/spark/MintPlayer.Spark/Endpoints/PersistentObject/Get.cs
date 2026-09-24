using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

internal sealed partial class GetPersistentObject : IPostEndpoint, IMemberOf<PersistentObjectGroup>
{
    public static string Path => "/load";

    // ⚠️ EXPLICITLY exempt, not merely unannotated. This is a read; a forged one changes nothing and
    // the attacker cannot see the response. It was exempt by absence until 11.0.0, when
    // SparkAntiforgeryOptions.RequireAntiforgery began defaulting to true and started gating any
    // mutating-verb request under /spark that carries an ambient credential — which swept these in
    // against the decision recorded above. Saying it out loud restores that decision and makes it
    // survive the next default change.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));
    }

    // ⚠️ Deliberately NO RequireAntiforgeryTokenAttribute, unlike every other POST in this group.
    // The verb changed; what the endpoint does did not. This is a read, and an antiforgery token
    // protects against a cross-site request causing a *change* — a load causes none, and a
    // cross-origin caller still cannot read the response. Requiring one here would break every
    // caller that legitimately reads without a session, for no property gained.
    //
    // The mutating endpoints in this file's neighbours keep theirs. If this endpoint ever gains a
    // side effect, it needs the attribute in the same commit.

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IRetryAccessor retryAccessor;
    [Inject] private readonly Abstractions.ClientOperations.IClientAccessor clientAccessor;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        // The catch-all `{**id}` that used to carry the id is gone, and with it the empty-id case it
        // created (the bare "/{objectTypeId}" path matched too, and the ! on a null RouteValue was a
        // 500 rather than a refusal). An absent id in the body is now just an absent field.
        var (request, entityType) = await SparkRequestType.ReadAsync<PersistentObjectReferenceRequest>(httpContext, modelLoader);
        if (request is null || entityType is null || string.IsNullOrEmpty(request.Id))
        {
            return SparkDenial.RefuseJson(httpContext);
        }

        // A load can prompt now, which is the whole reason it grew a body. OnLoadAsync raising a
        // retry gets the same 449 envelope as every other hook.
        RetryScope.Accept(retryAccessor, request);

        try
        {
            // No UnescapeDataString: see the note in Update.
            var obj = await databaseAccess.GetPersistentObjectAsync(entityType.Id, request.Id);

            if (obj is null)
            {
                return SparkDenial.RefuseJson(httpContext);
            }

            // Withholds issued through IClientAccessor used to be dropped here, silently: they ride
            // the client-operation envelope and this endpoint returned a bare JSON object, so an
            // author who called DisableActionsOn(po, ...) inside OnLoadAsync got no error and no
            // effect, while PersistentObject.DisableActions on the same object worked.
            //
            // Folded onto the object instead of switching this endpoint to the envelope: the client
            // already reads disabledActions off the PO, so this needs no wire change, and the two
            // APIs converge on one field rather than one growing a second delivery mechanism. That
            // reasoning survives the move to POST — the response is still a bare object.
            MergeClientWithholds(obj);

            // ⚠️ Still a bare object, not an envelope, even though this is a POST now. The verb moved
            // so that a load could carry a retry answer; the response shape is a separate decision
            // with its own blast radius (every consumer of this payload), and making both changes in
            // one commit would leave a failure in either indistinguishable from a failure in the
            // other. The 449 below is enveloped because a retry has nowhere else to live.
            return Results.Json(obj);
        }
        catch (SparkAccessDeniedException)
        {
            return SparkDenial.RefuseJson(httpContext);
        }
    }

    /// <summary>
    /// Folds every <see cref="IClientAccessor"/> withhold aimed at this object onto the object
    /// itself, so both APIs land in the same place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three target shapes reach a detail response. A PO target naming this object's type and id is
    /// obviously ours. A current-response target is ours because this endpoint IS the current
    /// response. A session target applies to everything the caller sees, so it applies here too.
    /// </para>
    /// <para>
    /// A query target is deliberately not folded in: it names a query's result view, which this is
    /// not, and quietly widening it to a detail page would disable an action somewhere its author
    /// never asked for.
    /// </para>
    /// <para>
    /// <b>This is an affordance, not a permission.</b> It removes a button; it does not refuse the
    /// action. The action's own right is what refuses it, and that is re-checked on execute.
    /// </para>
    /// </remarks>
    private void MergeClientWithholds(Abstractions.PersistentObject obj)
    {
        var names = clientAccessor.Operations
            .OfType<Abstractions.ClientOperations.DisableActionOperation>()
            .Where(op => op.Target switch
            {
                Abstractions.ClientOperations.PersistentObjectDisableTarget t
                    => t.ObjectTypeId == obj.ObjectTypeId
                       && string.Equals(t.Id, obj.Id, StringComparison.Ordinal),
                Abstractions.ClientOperations.CurrentResponseDisableTarget => true,
                Abstractions.ClientOperations.SessionDisableTarget => true,
                _ => false,
            })
            .Select(op => op.ActionName)
            .ToArray();

        if (names.Length > 0)
            obj.DisableActions(names);
    }
}
