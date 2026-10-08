using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.Retry;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Endpoints.PersistentObject;

[MemberOf<PersistentObjectGroup>]
internal sealed partial class GetPersistentObject : IPostEndpoint<PersistentObjectReferenceRequest>
{
    public static string Path => "/load";

    // ⚠️ EXPLICITLY exempt, not merely unannotated. This is a read; a forged one changes nothing and
    // the attacker cannot see the response. It was exempt by absence until 11.0.0, when
    // SparkAntiforgeryOptions.RequireAntiforgery began defaulting to true and started gating any
    // mutating-verb request under /spark that carries an ambient credential — which swept these in
    // against the decision recorded above. Saying it out loud restores that decision and makes it
    // survive the next default change.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
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
    [Inject] private readonly IDisabledActionsEvaluator disabledActions;
    [Inject] private readonly IRowPolicyRequestState rowPolicyRequestState;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>A body that cannot be bound gets the refusal an unusable request gets below, never a parse error (PRD D3a).</summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(SparkDenial.RefuseJson(context));

    public override async Task<IResult> HandleAsync(PersistentObjectReferenceRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        // The catch-all `{**id}` that used to carry the id is gone, and with it the empty-id case it
        // created (the bare "/{objectTypeId}" path matched too, and the ! on a null RouteValue was a
        // 500 rather than a refusal). An absent id in the body is now just an absent field.
        var entityType = SparkRequestType.Resolve(modelLoader, request);
        if (entityType is null || string.IsNullOrEmpty(request.Id))
        {
            return SparkDenial.RefuseJson(httpContext);
        }

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
            {
                return SparkDenial.RefuseJson(httpContext);
            }

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
}
