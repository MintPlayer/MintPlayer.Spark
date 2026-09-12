using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The one place in the codebase that answers <i>"which entity type is this request about?"</i>.
/// </summary>
/// <remarks>
/// <para>
/// The answer is used to authorize the request, so where it comes from is a security property, not a
/// plumbing detail. It must always be a value the <b>server</b> resolved — never one the client
/// asserted inside the payload it is submitting. Authorizing a write against the written object's own
/// self-declaration is the confused-deputy shape, and it is precisely what <c>Create.cs</c> overwrites
/// <c>persistentObject.objectTypeId</c> to prevent (security sweep C3: <i>"taking the client's word for
/// the type is how a caller reads one collection through another's permissions"</i>).
/// </para>
/// <para>
/// Today that value is the <c>{objectTypeId}</c> route segment, and this type is a single indirection
/// over nine copies of the same two lines. ⚠️ <b>That is the point.</b> The route table is becoming
/// fully literal (<c>POST /spark/po/create</c>), after which the type arrives as a top-level field in
/// the request body instead — and the distinction between <i>that</i> field and the nested
/// <c>persistentObject.objectTypeId</c> stops being visual. A route segment and a JSON body cannot be
/// confused; two fields one word apart in the same document can.
/// </para>
/// <para>
/// So when the source moves, it moves <b>here</b>, once, and every endpoint follows. The invariant is
/// pinned from the outside by
/// <c>tests/MintPlayer.Spark.Tests/Endpoints/PersistentObject/TypeConflationTests.cs</c> (a payload
/// naming a different type must not change which type is used, nor buy access to a denied one) and
/// from the inside by <c>SparkRequestTypeSingleSourceTests</c>, which fails if any endpoint goes back
/// to reading the route value or the nested field itself.
/// </para>
/// </remarks>
internal static class SparkRequestType
{
    /// <summary>The route segment every persistent-object, query and action endpoint carries today.</summary>
    internal const string RouteKey = "objectTypeId";

    /// <summary>
    /// Resolves the entity type this request is about, or <see langword="null"/> when the caller named
    /// one that does not exist. A null is a <b>refusal</b>, deliberately indistinguishable from a
    /// denial (M-3: a distinguishable "no such type" is a disclosure oracle for the type catalogue).
    /// </summary>
    public static EntityTypeDefinition? Resolve(IModelLoader modelLoader, HttpContext httpContext)
    {
        var value = httpContext.Request.RouteValues[RouteKey]?.ToString();

        return string.IsNullOrEmpty(value) ? null : modelLoader.ResolveEntityType(value);
    }
}
