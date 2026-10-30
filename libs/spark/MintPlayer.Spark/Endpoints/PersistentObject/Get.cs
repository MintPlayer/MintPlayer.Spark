using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Exceptions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Abstractions.Prerendering;

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
        builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false), SparkPrerenderableAttribute.Instance);
    }

    // ⚠️ Deliberately NO RequireAntiforgeryTokenAttribute, unlike every other POST in this group.
    // The verb changed; what the endpoint does did not. This is a read, and an antiforgery token
    // protects against a cross-site request causing a *change* — a load causes none, and a
    // cross-origin caller still cannot read the response. Requiring one here would break every
    // caller that legitimately reads without a session, for no property gained.
    //
    // The mutating endpoints in this file's neighbours keep theirs. If this endpoint ever gains a
    // side effect, it needs the attribute in the same commit.

    // The body lives in PersistentObjectReadPath, shared with the live socket (X4): a pushed object and
    // a loaded object are one read path.
    [Inject] private readonly IPersistentObjectReadPath readPath;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>A body that cannot be bound gets the refusal an unusable request gets below, never a parse error (PRD D3a).</summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(SparkDenial.RefuseJson(context));

    public override async Task<IResult> HandleAsync(PersistentObjectReferenceRequest request, CancellationToken cancellationToken)
    {
        var httpContext = httpContextAccessor.HttpContext!;
        var obj = await readPath.LoadAsync(request, httpContext.RequestAborted);

        // ⚠️ Still a bare object, not an envelope, even though this is a POST now. The verb moved
        // so that a load could carry a retry answer; the response shape is a separate decision
        // with its own blast radius (every consumer of this payload), and making both changes in
        // one commit would leave a failure in either indistinguishable from a failure in the
        // other. The 449 is enveloped by the middleware because a retry has nowhere else to live.
        return obj is null ? SparkDenial.RefuseJson(httpContext) : Results.Json(obj);
    }
}
