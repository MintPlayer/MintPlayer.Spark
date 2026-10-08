using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Requests;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The one place in the codebase that answers <i>"which entity type is this request about?"</i>.
/// </summary>
/// <remarks>
/// <para>
/// The answer is used to authorize the request, so where it comes from is a security property, not a
/// plumbing detail. It must always be a value the <b>server</b> resolved from the request's own
/// parameters — never one the client asserted inside the document it is submitting. Authorizing a write
/// against the written object's own self-declaration is the confused-deputy shape, and it is precisely
/// what the endpoints overwrite <c>persistentObject.objectTypeId</c> to prevent (security sweep C3:
/// <i>"taking the client's word for the type is how a caller reads one collection through another's
/// permissions"</i>).
/// </para>
/// <para>
/// The value used to be the <c>{objectTypeId}</c> route segment. Now that the route table is fully
/// literal (<c>POST /spark/po/create</c>), it is the top-level <see cref="ISparkTypedRequest.ObjectTypeId"/>
/// field of the request body. ⚠️ <b>That change is why this type exists.</b> A route segment and a JSON
/// body cannot be confused; the request parameter and the nested field are now one word apart in the
/// same document, so the distinction survives as a single reviewable line rather than as nine places
/// that each happen to be written correctly.
/// </para>
/// <para>
/// The invariant is pinned from the outside by
/// <c>tests/MintPlayer.Spark.Tests/Endpoints/PersistentObject/TypeConflationTests.cs</c> (a payload
/// naming a different type must not change which type is used, nor buy access to a denied one) and from
/// the inside by <c>SparkRequestTypeSingleSourceTests</c>, which fails if any endpoint resolves a type
/// itself.
/// </para>
/// <para>
/// ⚠️ <b>The body is read before anything can be authorized</b>, because the body is where the type is.
/// A body that cannot be bound is therefore answered exactly like an unknown type: every typed endpoint
/// gives the same refusal from <c>OnBindFailedAsync</c> that it gives a null from <see cref="Resolve"/>,
/// so a parse failure never tells an unauthorized caller which entity types exist (N23).
/// </para>
/// <para>
/// ⚠️ None of this may be left to <c>MintPlayer.Spark.Authorization</c> to enforce. That package is
/// optional and may be absent from the service container entirely; the resolved type decides which
/// collection is read and written, not merely which permission is consulted.
/// </para>
/// </remarks>
internal static class SparkRequestType
{
    /// <summary>
    /// Resolves the entity type <paramref name="request"/> is about, or <see langword="null"/> when it
    /// names none or names one that does not exist. A null is a <b>refusal</b>, deliberately
    /// indistinguishable from a denial (M-3: a distinguishable "no such type" is a disclosure oracle
    /// for the type catalogue).
    /// </summary>
    public static EntityTypeDefinition? Resolve(IModelLoader modelLoader, ISparkTypedRequest? request)
        => string.IsNullOrEmpty(request?.ObjectTypeId)
            ? null
            : modelLoader.ResolveEntityType(request.ObjectTypeId);
}
